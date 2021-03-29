//
//  KeyFilePicker.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 3/27/21.
//  Copyright © 2021 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit
import MobileCoreServices

extension KeyFiles {
    fileprivate class KeyFilePicker: UIDocumentPickerViewController, UIDocumentPickerDelegate {
        
        var completionHandler: ((Result<Void, Error>) -> Void)? = nil
        public func documentPicker(_ controller: UIDocumentPickerViewController, didPickDocumentsAt urls: [URL]) {
            guard let url = urls.first else {
                return
            }
            if url.isFileURL {
                let shouldStopAccessing = url.startAccessingSecurityScopedResource()
                defer {
                    if shouldStopAccessing {
                        url.stopAccessingSecurityScopedResource()
                    }
                }
                
                let path = url.path
                if  KeyFiles.validateKeyFile(url: url) {
                    do {
                        if FileManager.default.fileExists(atPath: KeyFiles.documentsKeyPath.path) {
                            try FileManager.default.removeItem(at: KeyFiles.documentsKeyPath)
                        }
                        
                        try FileManager.default.copyItem(at: url, to: KeyFiles.documentsKeyPath)
                        
                        if KeyFiles.LoadKeys() {
                            self.completionHandler?(.success(()))
                        } else {
                            self.completionHandler?(.failure(AmiiTagError(description: "Error loading key file")))
                        }
                    } catch {
                        self.completionHandler?(.failure(error))
                    }
                } else {
                    self.completionHandler?(.failure(AmiiTagError(description: "Invalid key file")))
                }
            }
        }
        
        fileprivate override init(documentTypes allowedUTIs: [String], in mode: UIDocumentPickerMode) {
            super.init(documentTypes: allowedUTIs, in: mode)
        }
        
        required init?(coder: NSCoder) {
            super.init(coder: coder)
        }
    }
    
    static func pickKeyFile(PresentingViewController presentingVc: UIViewController, completionHandler: @escaping (Result<Void, Error>) -> Void){
        var pickerController = KeyFilePicker(documentTypes: [kUTTypeData as String], in: .open)
        pickerController.completionHandler = completionHandler
        pickerController.delegate = pickerController
        pickerController.allowsMultipleSelection = false
        presentingVc.present(pickerController, animated: true)
    }
}

