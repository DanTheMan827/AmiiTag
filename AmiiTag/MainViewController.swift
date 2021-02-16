//
//  MainView.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 6/28/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit
import CoreNFC
import MobileCoreServices
import SwiftyBluetooth

class MainViewController: UIViewController, NFCTagReaderSessionDelegate, UIDocumentPickerDelegate, ScannerViewControllerDelegate, LibraryPickerProtocol {
    static var main: MainViewController?
    func AmiiboSeriesPicked(series: String) -> Bool {
        return true
    }
    
    func AmiiboCharacterPicked(tag: TagDump) -> Bool {
        return true
    }
    
    @IBOutlet var logo: UIImageView!
    
    func scannerCodeFound(code: String) {
        guard let data = try? Data(base64Encoded: code, options: .ignoreUnknownCharacters) else {
            return
        }
        
        if (data.count == 532 || data.count == 540 || data.count == 572) {
            if let dump = TagDump(data: data) {
                dismiss(animated: true) {
                    TagInfoViewController.openTagInfo(dump: dump, controller: self)
                }
            }
        }
    }
    
    var tagReaderSession: NFCTagReaderSession?
    var pickerController: UIDocumentPickerViewController!
    
    @IBAction func loadTagTap(_ sender: Any) {
        self.present(pickerController, animated: true)
    }
    
    @IBAction func startTagReadingSession() {
        if (PuckPeripheral.pucks.count > 0) {
            let alertController = UIAlertController(title: nil, message: nil, preferredStyle: .actionSheet)
            alertController.view.tintColor = self.view.tintColor
            
            for puck in PuckPeripheral.pucks.sorted(by: { (a, b) -> Bool in
                return a.name > b.name
            }) {
                alertController.addAction(UIAlertAction(title: puck.name, style: .default, handler: { (action) in
                    let alert = UIAlertController(title: "Please Wait", message: "Reading " + (puck.name), preferredStyle: .alert)
                    self.present(alert, animated: true)
                    
                    puck.readTag { (result) in
                        switch result {
                        case .success(let tag):
                            self.dismiss(animated: true)
                            TagInfoViewController.openTagInfo(dump: TagDump(data: tag)!, controller: self)
                            break
                        case .failure(let error):
                            self.dismiss(animated: true)
                            let errorAlert = UIAlertController(title: "Oh no!", message: error.localizedDescription, preferredStyle: .alert)
                            errorAlert.addAction(UIAlertAction(title: "OK", style: .default, handler: nil))
                            self.present(errorAlert, animated: true)
                            break
                        }
                        
                        puck.disconnect { (result) in
                            PuckPeripheral.startScanning()
                        }
                    }
                    
                }))
            }
            
            alertController.addAction(UIAlertAction(title: "NFC", style: .default){ action -> Void in
                self.startNfcTagReadingSession()
            })
            
            alertController.addAction(UIAlertAction(title: "Cancel", style: .cancel){ action -> Void in })
            self.present(alertController, animated:true){}
        } else {
            self.startNfcTagReadingSession()
        }
    }
    
    func startNfcTagReadingSession() {
        tagReaderSession = NFCTagReaderSession(pollingOption: NFCTagReaderSession.PollingOption.iso14443, delegate: self)
        tagReaderSession?.alertMessage = "Hold tag to back of phone!"
        tagReaderSession?.begin()
    }
    
    @IBAction func scanQrTap(_ sender: Any) {
        let storyboard = UIStoryboard(name: "Main", bundle: nil)
        guard let vc = storyboard.instantiateViewController(withIdentifier: "ScannerView") as? ScannerViewController else {
            return
        }
        vc.delegate = self
        self.present(vc, animated: true)
    }
    
    @IBAction func managePuckTap(_ sender: Any) {
        if let alertController = PuckPeripheral.getPuckChooser(puckChosen: { (puck) in
            let alert = UIAlertController(title: "Please Wait", message: "Reading " + (puck.name), preferredStyle: .alert)
            self.present(alert, animated: true)
            
            puck.getAllSlotInformation { (result) in
                self.dismiss(animated: true, completion: nil)
                switch result {
                case .success(let data):
                    let storyboard = UIStoryboard(name: "Main", bundle: nil)
                    guard let view = storyboard.instantiateViewController(withIdentifier: "AmiiboCharactersPuck") as? AmiiboCharactersPuckTableViewController else {
                        puck.disconnect { (result) in }
                        return
                    }
                    
                    view.puck = puck
                    view.puckSlots = data
                    view.title = puck.name
                    let navigationController = UINavigationController(rootViewController: view)
                    navigationController.setToolbarHidden(false, animated: false)
                    self.present(navigationController, animated: true, completion: nil)
                    
                    break
                case .failure(let error):
                    let errorAlert = UIAlertController(title: "Oh no!", message: error.localizedDescription, preferredStyle: .alert)
                    errorAlert.addAction(UIAlertAction(title: "OK", style: .default, handler: nil))
                    self.present(errorAlert, animated: true, completion: nil)
                    puck.disconnect { (result) in }
                    break
                }
            }
        }) {
            present(alertController, animated: true, completion: nil)
        }
    }
    
    @IBAction func amiiboLibraryTap(_ sender: Any) {
        LibraryPicker.ShowPicker(using: self, with: self)
    }
    
    func handleConnectedTag(tag: NFCMiFareTag) {
        NTAG215Tag.initialize(tag: tag) { result in
            switch result {
            case .success(let ntag215Tag):
                self.tagReaderSession?.invalidate()
                DispatchQueue.main.async {
                    print(ntag215Tag.dump.TagUIDSig)
                    self.writeUidSig(data: ntag215Tag.dump.TagUIDSig!)
                    TagInfoViewController.openTagInfo(dump: ntag215Tag.dump, controller: self)
                }
            case .failure(let error):
                self.tagReaderSession?.invalidate(errorMessage: error.localizedDescription)
            }
        }
    }
    
    func writeUidSig(data: Data) {
        let fileManager = FileManager.default
        let documentsURL =  fileManager.urls(for: .documentDirectory, in: .userDomainMask).first!

        let sigPath = documentsURL.appendingPathComponent("signatures")
        do
        {
            try FileManager.default.createDirectory(atPath: sigPath.path, withIntermediateDirectories: true, attributes: nil)
            try data.write(to: sigPath.appendingPathComponent("\(data[0..<9].map { String(format: "%02hhx", $0) }.joined()).bin"))
        }
        catch let error as NSError
        {
            NSLog("Unable to create directory \(error.debugDescription)")
        }
    }
    
    override func viewDidLoad() {
        super.viewDidLoad()
        
        MainViewController.main = self
        
        print("Loaded \(NTAG215Tag.uidSignatures.count) UID/Signature pairs")
        print("Loaded \(AmiiboDatabase.amiiboDumps.count) amiibo dumps")
        
        pickerController = UIDocumentPickerViewController(documentTypes: [kUTTypeData as String], in: .open)
        pickerController.delegate = self
        pickerController.allowsMultipleSelection = false
    }
    
    // MARK: DocumentDelegate
    public func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
        guard let url = urls.first else {
            return
        }
        if url.isFileURL {
            let shouldStopAccessing = url.startAccessingSecurityScopedResource()
            let path = url.path
            if let attr = try? FileManager.default.attributesOfItem(atPath: path) as NSDictionary {
                if attr.fileSize() == 532 || attr.fileSize() == 540 || attr.fileSize() == 572 {
                    if let data = try? Data(contentsOf: url),
                       let dump = TagDump(data: data) {
                        dismiss(animated: true) {
                            TagInfoViewController.openTagInfo(dump: dump, controller: self)
                        }
                    }
                    
                }
            }
            if shouldStopAccessing {
                url.stopAccessingSecurityScopedResource()
            }
        }
    }
    
    // MARK: NFCTagReaderSessionDelegate
    func tagReaderSessionDidBecomeActive(_ session: NFCTagReaderSession) {
        NSLog("tagReaderSessionDidBecomeActive")
    }
    
    func tagReaderSession(_ session: NFCTagReaderSession, didInvalidateWithError error: Error) {
        NSLog("NFCTagReaderSession, didInvalidateWithError \(error)")
    }
    
    func tagReaderSession(_ session: NFCTagReaderSession, didDetect tags: [NFCTag]) {
        guard case let NFCTag.miFare(tag) = tags.first! else {
            tagReaderSession?.invalidate(errorMessage: "Invalid tag type.")
            return
        }
        session.connect(to: tags.first!) { (error: Error?) in
            if let error = error {
                session.invalidate(errorMessage: error.localizedDescription)
            } else {
                self.handleConnectedTag(tag: tag)
            }
        }
    }
}
