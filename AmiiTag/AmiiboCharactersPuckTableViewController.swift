//
//  AmiiboCharactersPuckTableViewController.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 2/14/21.
//  Copyright © 2021 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit
import MobileCoreServices
import SwiftyBluetooth

class AmiiboCharactersPuckTableViewController: UITableViewController {
    var puck: PuckPeripheral! = nil
    var puckSlots: [PuckPeripheral.SlotInfo] = []
    var cells: [AmiiboCharacterPuckTableViewCell] = []
    static var showing = false
    
    // MARK: UIViewController
    override func viewDidLoad() {
        super.viewDidLoad()
        NotificationCenter.default.addObserver(forName: Peripheral.PeripheralDisconnected, object: puck.peripheral, queue: nil) { (notification) in
            if AmiiboCharactersPuckTableViewController.showing {
                MainViewController.main?.dismiss(animated: true)
            }
        }
    }
    
    override func viewDidDisappear(_ animated: Bool) {
        AmiiboCharactersPuckTableViewController.showing = false
        MainViewController.main?.present(UIAlertController(title: "Please Wait", message: "Disconnecting from \(puck.name)", preferredStyle: .alert), animated: true)
        self.puck.disconnect { (result) in
            MainViewController.main?.dismiss(animated: true)
        }
    }
    
    override func viewDidAppear(_ animated: Bool) {
        AmiiboCharactersPuckTableViewController.showing = true
    }
    
    // MARK: UITableViewDataSource
    override func numberOfSections(in tableView: UITableView) -> Int {
        return 1
    }
    override func tableView(_ tableView: UITableView, numberOfRowsInSection section: Int) -> Int {
        return puckSlots.count
    }
    override func tableView(_ tableView: UITableView, cellForRowAt indexPath: IndexPath) -> UITableViewCell {
        let index = (indexPath as NSIndexPath).row
        if let tableView = self.view as? UITableView {
            let cell = tableView.dequeueReusableCell(withIdentifier: "CharacterPuckTableCell") as! AmiiboCharacterPuckTableViewCell
            
            cell.ViewController = self
            cell.Info = puckSlots[index]
            cell.Puck = puck
            cell.CellLabel.text = puckSlots[index].dump.displayName
            if !puckSlots[index].dump.fullHex.hasSuffix("02") {
                cell.CellLabel.text = "Unknown Data"
            }
            cell.CellImage.image = puckSlots[index].dump.image
            cells.append(cell)
            
            return cell
        }
        
        return UITableViewCell(frame: CGRect.zero)
    }
    override func tableView(_ tableView: UITableView, heightForRowAt indexPath: IndexPath) -> CGFloat {
        return 100.0
    }
    override func tableView(_ tableView: UITableView, leadingSwipeActionsConfigurationForRowAt indexPath: IndexPath) -> UISwipeActionsConfiguration? {
        
        let randomizeAction = UIContextualAction(style: .destructive, title: nil) {  (contextualAction, view, boolValue) in
            if let cell = (self.view as? UITableView)?.cellForRow(at: indexPath) as? AmiiboCharacterPuckTableViewCell {
                cell.randomizeTapped(self)
            }
            
            boolValue(true)
        }
        
        let awesomeLabel = UILabel()
        awesomeLabel.font = UIFont(name: "Font Awesome 6 Free Solid", size: 24)
        awesomeLabel.textColor = UIColor.white
        awesomeLabel.text = "" // Random
        awesomeLabel.sizeToFit()
        
        randomizeAction.backgroundColor = UIColor.systemBlue
        randomizeAction.image = UIImage(view: awesomeLabel)
        let swipeActions = UISwipeActionsConfiguration(actions: [randomizeAction])

        return swipeActions
    }
    override func tableView(_ tableView: UITableView, trailingSwipeActionsConfigurationForRowAt indexPath: IndexPath) -> UISwipeActionsConfiguration? {
        let uploadAction = UIContextualAction(style: .destructive, title: nil) {  (contextualAction, view, boolValue) in
            if let cell = (self.view as? UITableView)?.cellForRow(at: indexPath) as? AmiiboCharacterPuckTableViewCell {
                cell.uploadTapped(self)
            }
            
            boolValue(true)
        }
        let downloadAction = UIContextualAction(style: .destructive, title: nil) {  (contextualAction, view, boolValue) in
            if let cell = (self.view as? UITableView)?.cellForRow(at: indexPath) as? AmiiboCharacterPuckTableViewCell {
                cell.downloadTapped(self)
            }
            
            boolValue(true)
        }
        let clearAction = UIContextualAction(style: .destructive, title: nil) {  (contextualAction, view, boolValue) in
            if let cell = (self.view as? UITableView)?.cellForRow(at: indexPath) as? AmiiboCharacterPuckTableViewCell {
                cell.clearTapped(self)
            }
            
            boolValue(true)
        }
        
        var awesomeLabel = UILabel()
        awesomeLabel.font = UIFont(name: "Font Awesome 6 Free Solid", size: 24)
        awesomeLabel.textColor = UIColor.white
        
        awesomeLabel.text = "" // Download
        awesomeLabel.sizeToFit()
        downloadAction.image = UIImage(view: awesomeLabel)
        
        awesomeLabel.text = "" // Upload
        awesomeLabel.sizeToFit()
        uploadAction.image = UIImage(view: awesomeLabel)
        
        awesomeLabel.text = "" // Trash-Alt
        awesomeLabel.sizeToFit()
        awesomeLabel.textColor = UIColor.white
        clearAction.image = UIImage(view: awesomeLabel)
        
        
        uploadAction.backgroundColor = UIColor.systemBlue
        downloadAction.backgroundColor = UIColor.systemBlue
        let swipeActions = UISwipeActionsConfiguration(actions: [clearAction, downloadAction, uploadAction])

        swipeActions.performsFirstActionWithFullSwipe = false
        return swipeActions
    }
    
    // MARK: UITableViewDelegate
    override func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        if let cell = (self.view as? UITableView)?.cellForRow(at: indexPath) as? AmiiboCharacterPuckTableViewCell {
            (self.view as? UITableView)?.deselectRow(at: indexPath, animated: true)
            let alert = UIAlertController(title: "Please Wait", message: "Changing Slot", preferredStyle: .alert)
            self.present(alert, animated: true)
            puck.changeSlot(slot: cell.Info.slot) { (result) in
                self.dismiss(animated: true)
            }
        }
    }
    
    @IBAction func settingsTapped(_ sender: Any) {
        let alertController = UIAlertController(title: puck.name , message: nil, preferredStyle: .actionSheet).Popify(view: self.view)
        
        alertController.addAction(UIAlertAction(title: "Change Name", style: .default, handler: { (action) in
            let alert = UIAlertController(title: self.puck.name, message: nil, preferredStyle: .alert)
            alert.addAction(UIAlertAction(title: "Cancel", style: .cancel, handler: nil))

            alert.addTextField(configurationHandler: { textField in
                textField.placeholder = "Enter new puck name"
            })

            alert.addAction(UIAlertAction(title: "OK", style: .default, handler: { action in
                self.present(UIAlertController(title: "Please Wait", message: "Changing Name", preferredStyle: .alert), animated: true)
                self.puck.changeName(name: alert.textFields?.first?.text ?? "") { (result) in
                    switch result {
                    case .success(()):
                        self.dismiss(animated: true)
                        MainViewController.main?.dismiss(animated: true)
                        PuckPeripheral.stopScanning()
                        PuckPeripheral.startScanning()
                        break
                    case .failure(let error):
                        self.dismiss(animated: true) {
                            self.present(error.getAlertController(), animated: true)
                        }
                        break
                    }
                }
            }))

            self.present(alert, animated: true)
        }))
        
        alertController.addAction(UIAlertAction(title: "Enable Uart", style: .default, handler: { (action) in
            self.puck.enableUart(completionHandler: { (result) in
                switch result {
                case .success(()):
                    self.puck.disconnect { (result) in }
                    break
                case .failure(let error):
                    self.dismiss(animated: true) {
                        self.present(error.getAlertController(), animated: true)
                    }
                    break
                }
            })
        }))
        
        alertController.addAction(UIAlertAction(title: "Cancel", style: .cancel))
        
        self.present(alertController, animated:true){}
    }
    
}
