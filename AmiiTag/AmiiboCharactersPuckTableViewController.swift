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

class AmiiboCharactersPuckTableViewController: UITableViewController {
    var puck: PuckPeripheral! = nil
    var puckSlots: [PuckPeripheral.SlotInfo] = []
    var cells: [AmiiboCharacterPuckTableViewCell] = []
    
    // MARK: UIViewController
    override func viewDidLoad() {
        super.viewDidLoad()
    }
    
    override func viewDidDisappear(_ animated: Bool) {
        self.puck.disconnect { (result) in }
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
            cell.CellImage.image = puckSlots[index].dump.image
            cells.append(cell)
            
            return cell
        }
        
        return UITableViewCell(frame: CGRect.zero)
    }
    override func tableView(_ tableView: UITableView, heightForRowAt indexPath: IndexPath) -> CGFloat {
        return 100.0
    }
    
    // MARK: UITableViewDelegate
    override func tableView(_ tableView: UITableView, didSelectRowAt indexPath: IndexPath) {
        if let cell = (self.view as? UITableView)?.cellForRow(at: indexPath) as? AmiiboCharacterPuckTableViewCell {
            (self.view as? UITableView)?.deselectRow(at: indexPath, animated: true)
            let alert = UIAlertController(title: "Please Wait", message: "Changing Slot", preferredStyle: .alert)
            self.present(alert, animated: true, completion: nil)
            puck.changeSlot(slot: cell.Info.slot) { (result) in
                self.dismiss(animated: true, completion: nil)
            }
        }
    }
    
    @IBAction func settingsTapped(_ sender: Any) {
        let alertController = UIAlertController(title: puck.name ?? "Puck", message: nil, preferredStyle: .actionSheet)
        
        alertController.addAction(UIAlertAction(title: "Change Name", style: .default, handler: { (action) in
            let alert = UIAlertController(title: self.puck.name, message: nil, preferredStyle: .alert)
            alert.addAction(UIAlertAction(title: "Cancel", style: .cancel, handler: nil))

            alert.addTextField(configurationHandler: { textField in
                textField.placeholder = "Enter new puck name"
            })

            alert.addAction(UIAlertAction(title: "OK", style: .default, handler: { action in
                self.present(UIAlertController(title: "Please Wait", message: "Changing Name", preferredStyle: .alert), animated: true, completion: nil)
                self.puck.changeName(name: alert.textFields?.first?.text ?? "") { (result) in
                    switch result {
                    case .success(()):
                        self.dismiss(animated: true, completion: nil)
                        MainViewController.main?.dismiss(animated: true, completion: nil)
                        PuckPeripheral.stopScanning()
                        PuckPeripheral.startScanning()
                        break
                    case .failure(let error):
                        self.dismiss(animated: true)
                        let errorAlert = UIAlertController(title: "Oh no!", message: error.localizedDescription, preferredStyle: .alert)
                        errorAlert.addAction(UIAlertAction(title: "OK", style: .default, handler: nil))
                        self.present(errorAlert, animated: true)
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
                    self.dismiss(animated: true)
                    let errorAlert = UIAlertController(title: "Oh no!", message: error.localizedDescription, preferredStyle: .alert)
                    errorAlert.addAction(UIAlertAction(title: "OK", style: .default, handler: nil))
                    self.present(errorAlert, animated: true)
                    break
                }
            })
        }))
        
        alertController.addAction(UIAlertAction(title: "Cancel", style: .cancel))
        
        self.present(alertController, animated:true){}
    }
    
}
